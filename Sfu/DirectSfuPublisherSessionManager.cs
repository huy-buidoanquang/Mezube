using Mezube.Bot;
using Mezube.Media;
using Mezon.Net.Sdk;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

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
    private Exception? _terminalFailure;
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
        if (!SfuMediaSource.TryParse(mediaUrl, out var media))
        {
            throw new ArgumentException("SFU playback requires a local .ogg/.opus file or an absolute HTTP(S) media URL.", nameof(mediaUrl));
        }

        await StartAsync(token, tokenProvider, cancellationToken).ConfigureAwait(false);
        BeginStream(trackId, media);
    }

    internal void BeginStream(string trackId, SfuMediaSource media)
    {
        if (string.IsNullOrWhiteSpace(trackId))
        {
            throw new ArgumentException("An SFU track id is required.", nameof(trackId));
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
            _ = StreamAudioAsync(trackId, media, _trackCts.Token);
        }
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
        var reconnectAttempts = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await ConnectAndRunAsync(token, cancellationToken).ConfigureAwait(false);
            if (result.WasStable)
            {
                reconnectAttempts = 0;
            }

            if (result.Failure is not null)
            {
                _logger.LogWarning(
                    "SFU publisher connection failed channel={ChannelId}; reconnecting error_type={ErrorType} error={Error}",
                    ChannelId,
                    result.Failure.GetType().Name,
                    result.Failure.Message);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            string? refreshedToken = null;
            while (refreshedToken is null && !cancellationToken.IsCancellationRequested)
            {
                if (reconnectAttempts >= _options.SfuReconnectMaxAttempts)
                {
                    FailSession(new InvalidOperationException(
                        $"SFU publisher reconnect limit reached after {_options.SfuReconnectMaxAttempts} attempts."));
                    return;
                }

                reconnectAttempts++;
                ResetConnectionSignal();
                var delay = SfuReconnectPolicy.GetDelay(
                    reconnectAttempts,
                    _options.SfuReconnectBackoffMs,
                    _options.SfuReconnectMaxBackoffMs,
                    _options.SfuReconnectJitterMs);
                _logger.LogWarning(
                    "SFU publisher reconnect scheduled channel={ChannelId} attempt={Attempt}/{MaxAttempts} delay_ms={DelayMs}",
                    ChannelId,
                    reconnectAttempts,
                    _options.SfuReconnectMaxAttempts,
                    (int)delay.TotalMilliseconds);

                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var provider = GetTokenProvider();
                if (provider is null)
                {
                    FailSession(new InvalidOperationException("SFU publisher token provider is unavailable."));
                    return;
                }

                try
                {
                    // Never reuse the previous JWT after a WebSocket/PeerConnection failure.
                    refreshedToken = await provider(cancellationToken).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(refreshedToken))
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
                        "SFU publisher token refresh failed channel={ChannelId} error_type={ErrorType} error={Error}",
                        ChannelId,
                        ex.GetType().Name,
                        ex.Message);
                }
            }

            if (refreshedToken is not null)
            {
                token = refreshedToken;
            }
        }
    }

    private readonly record struct ConnectionRunResult(bool WasStable, Exception? Failure);

    private async Task<ConnectionRunResult> ConnectAndRunAsync(
        string token,
        CancellationToken cancellationToken)
    {
        RTCPeerConnection? peerConnection = null;
        DirectSfuConnection? connection = null;
        Exception? failure = null;
        var wasStable = false;
        try
        {
            using var websocket = new ClientWebSocket();
            websocket.Options.SetRequestHeader("User-Agent", "mezube/1.0");
            await websocket.ConnectAsync(new Uri(_options.SfuWebSocketUrl), cancellationToken).ConfigureAwait(false);

            await SendJsonAsync(
                    websocket,
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
            var heartbeatTask = HeartbeatLoopAsync(websocket, heartbeatCts.Token);
            try
            {
                while (true)
                {
                    var message = await ReceiveTextAsync(websocket, cancellationToken).ConfigureAwait(false);
                    if (message is null)
                    {
                        throw connection?.Failure ?? new InvalidOperationException("SFU signaling connection closed.");
                    }

                    if (connection is null)
                    {
                        connection = TryAttachAfterJoined(websocket, message, cancellationToken, out peerConnection);
                        if (connection is not null)
                        {
                            continue;
                        }

                        await HandlePreJoinMessageAsync(websocket, message, cancellationToken).ConfigureAwait(false);
                        continue;
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            wasStable = connection is not null
                && connection.HasBeenConnected
                && connection.ConnectedDuration >= TimeSpan.FromMilliseconds(_options.SfuReconnectStableResetMs);
            connection?.Close();
            peerConnection?.Dispose();
            lock (_gate)
            {
                if (ReferenceEquals(_connection, connection))
                {
                    _connection = null;
                }
            }
        }

        return new ConnectionRunResult(wasStable, failure);
    }

    internal static RTCPeerConnection CreateAudioPublisherPeerConnection(
        IReadOnlyList<RTCIceServer>? iceServers = null)
    {
        RTCConfiguration? config = null;
        if (iceServers is { Count: > 0 })
        {
            config = new RTCConfiguration { iceServers = [.. iceServers] };
        }

        var peerConnection = new RTCPeerConnection(config);
        var audioTrack = new MediaStreamTrack(
            new List<AudioFormat> { new(AudioCodecsEnum.OPUS, 111, 48000, 2) },
            MediaStreamStatusEnum.SendOnly)
        {
            NoDtmfSupport = true
        };
        peerConnection.addTrack(audioTrack);
        return peerConnection;
    }

    internal static IReadOnlyList<RTCIceServer> ParseIceServers(JsonElement message)
    {
        if (!message.TryGetProperty("iceServers", out var servers)
            || servers.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var parsed = new List<RTCIceServer>();
        foreach (var server in servers.EnumerateArray())
        {
            var urls = ReadIceUrls(server);
            if (string.IsNullOrWhiteSpace(urls))
            {
                continue;
            }

            parsed.Add(new RTCIceServer
            {
                urls = urls,
                username = server.TryGetProperty("username", out var user) ? user.GetString() : null,
                credential = server.TryGetProperty("credential", out var credential) ? credential.GetString() : null,
            });
        }

        return parsed;
    }

    private static string? ReadIceUrls(JsonElement server)
    {
        if (!server.TryGetProperty("urls", out var urls))
        {
            return null;
        }

        if (urls.ValueKind == JsonValueKind.String)
        {
            return urls.GetString();
        }

        if (urls.ValueKind == JsonValueKind.Array && urls.GetArrayLength() > 0)
        {
            return urls[0].GetString();
        }

        return null;
    }

    private DirectSfuConnection? TryAttachAfterJoined(
        ClientWebSocket websocket,
        string payload,
        CancellationToken cancellationToken,
        out RTCPeerConnection? peerConnection)
    {
        peerConnection = null;
        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("type", out var typeElement)
            || !string.Equals(typeElement.GetString(), "joined", StringComparison.Ordinal))
        {
            return null;
        }

        var iceServers = ParseIceServers(document.RootElement);
        peerConnection = CreateAudioPublisherPeerConnection(iceServers);
        var connection = new DirectSfuConnection(websocket, peerConnection);
        lock (_gate)
        {
            if (_stopped)
            {
                connection.Close();
                peerConnection.Dispose();
                peerConnection = null;
                return null;
            }

            _connection = connection;
        }

        _logger.LogDebug(
            "SFU publisher joined channel={ChannelId} iceServers={IceServerCount}",
            ChannelId,
            iceServers.Count);
        AttachConnectionStateHandler(connection, cancellationToken);
        return connection;
    }

    private void AttachConnectionStateHandler(DirectSfuConnection connection, CancellationToken cancellationToken)
    {
        connection.PeerConnection.onconnectionstatechange += state =>
        {
            if (state == RTCPeerConnectionState.connected)
            {
                connection.MarkConnected();
                lock (_gate)
                {
                    _connected.TrySetResult();
                }

                _ = SendMuteAfterConnectAsync(connection, cancellationToken);
                return;
            }

            // SIPSorcery fires `closed` after Close()/Dispose. Treating that as a
            // transport failure Abort()s the signaling WS, which emits speaker
            // leave and steals the web-client's singleton user_id presence slot.
            if (state != RTCPeerConnectionState.failed)
            {
                return;
            }

            _logger.LogWarning(
                "SFU WebRTC ICE failed channel={ChannelId} ice={IceState}",
                ChannelId,
                connection.PeerConnection.iceConnectionState);
            connection.SetFailure(new InvalidOperationException("SFU WebRTC connection failed."));
            connection.Close();
        };
    }

    private async Task HandlePreJoinMessageAsync(
        ClientWebSocket websocket,
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
                await SendJsonAsync(websocket, new { type = "pong" }, cancellationToken).ConfigureAwait(false);
                break;
            case "pong":
                break;
            case "offer":
                throw new InvalidOperationException("SFU sent an offer before joined.");
            case "error":
                throw CreateSfuSignalingException(document.RootElement);
        }
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
                throw CreateSfuSignalingException(document.RootElement);
        }
    }

    private static InvalidOperationException CreateSfuSignalingException(JsonElement message)
    {
        var detail = message.TryGetProperty("message", out var detailElement)
            ? detailElement.GetString()
            : null;
        return new InvalidOperationException(
            string.IsNullOrWhiteSpace(detail)
                ? "SFU rejected publisher signaling."
                : $"SFU rejected publisher signaling: {detail}");
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
        // Renegotiation offers (peer join/leave) must not call Start() again.
        // SIPSorcery treats a second Start() as a new ICE gathering cycle and
        // can close the live pair, which Mezube then Abort()s as a reconnect.
        if (!connection.PeerConnection.IsAudioStarted)
        {
            await connection.PeerConnection.Start().ConfigureAwait(false);
        }
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

    private async Task StreamAudioAsync(string trackId, SfuMediaSource media, CancellationToken cancellationToken)
    {
        try
        {
            if (media.LocalPath is string localPath)
            {
                await using var file = new FileStream(
                    localPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await PumpOpusAsync(trackId, file, cancellationToken).ConfigureAwait(false);
                return;
            }

            using var response = await _httpClient.GetAsync(
                    media.HttpUri!,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await PumpOpusAsync(trackId, body, cancellationToken).ConfigureAwait(false);
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

    private async Task PumpOpusAsync(string trackId, Stream body, CancellationToken cancellationToken)
    {
        var reader = new OggOpusReader(body);
        // Keep at most ~80 ms of Opus (4 x 20 ms) decoded ahead of the pacer.
        // A 64-packet buffer could hold 1.28 s; after a GC stall that backlog
        // would sit ready and then hit the browser jitter buffer as a burst.
        var packets = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(4)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var fillTask = FillOpusPacketsAsync(reader, packets.Writer, cancellationToken);
        Exception? pumpError = null;

        try
        {
            await WaitForConnectedConnectionAsync(cancellationToken).ConfigureAwait(false);
            await Task.Factory.StartNew(
                    () => PumpOpusSendLoop(packets.Reader, cancellationToken),
                    cancellationToken,
                    TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            pumpError = ex;
        }
        finally
        {
            packets.Writer.TryComplete();
        }

        try
        {
            await fillTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            pumpError ??= ex;
        }

        if (pumpError is not null)
        {
            ExceptionDispatchInfo.Throw(pumpError);
        }

        CompleteTrack(trackId);
    }

    private void PumpOpusSendLoop(ChannelReader<byte[]> packets, CancellationToken cancellationToken)
    {
        var pacer = new OpusRtpPacer();
        pacer.Start();

        while (packets.WaitToReadAsync(cancellationToken).AsTask().GetAwaiter().GetResult())
        {
            while (IsPaused)
            {
                pacer.Pause();
                if (cancellationToken.WaitHandle.WaitOne(20))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            pacer.Resume();
            if (!packets.TryRead(out var packet))
            {
                continue;
            }

            var duration = OpusPacket.GetDurationSamples(packet);
            var connection = GetConnection();
            if (connection is not { IsConnected: true })
            {
                connection = WaitForConnectedConnectionAsync(cancellationToken)
                    .GetAwaiter()
                    .GetResult();
                pacer.Reset();
            }

            // Wait-then-send: first packet is due at t=0, each later packet at
            // cumulative samples / 48 kHz. mezon-sfu forwards audio immediately,
            // so a send-side burst is a receive-side JB overrun.
            while (true)
            {
                pacer.Wait(cancellationToken);
                try
                {
                    connection.PeerConnection.SendAudio(duration, packet);
                    break;
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    // Do not drop the packet that was already removed from the
                    // bounded queue. The connection is the failed resource;
                    // the Ogg reader and RTP pacing timeline remain owned by
                    // this track and resume with this same packet.
                    _logger.LogDebug(
                        "SFU audio send interrupted channel={ChannelId} error_type={ErrorType}",
                        ChannelId,
                        ex.GetType().Name);
                    connection.SetFailure(ex);
                    connection.Close();
                    connection = WaitForReplacementConnectionAsync(connection, cancellationToken)
                        .GetAwaiter()
                        .GetResult();
                    pacer.Reset();
                }
            }

            pacer.Account(duration);
            pacer.ReleaseCatchUp();
        }
    }

    private static async Task FillOpusPacketsAsync(
        OggOpusReader reader,
        ChannelWriter<byte[]> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var packet = await reader.ReadPacketAsync(cancellationToken).ConfigureAwait(false);
                if (packet is null)
                {
                    writer.TryComplete();
                    return;
                }

                await writer.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            writer.TryComplete();
            throw;
        }
        catch (ChannelClosedException)
        {
            return;
        }
        catch (Exception ex)
        {
            writer.TryComplete(ex);
            throw;
        }
    }

    private async Task<DirectSfuConnection> WaitForConnectedConnectionAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var terminalFailure = GetTerminalFailure();
            if (terminalFailure is not null)
            {
                throw new InvalidOperationException("SFU publisher reconnect is no longer available.", terminalFailure);
            }

            if (GetConnection() is { IsConnected: true } connection)
            {
                return connection;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<DirectSfuConnection> WaitForReplacementConnectionAsync(
        DirectSfuConnection previous,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var terminalFailure = GetTerminalFailure();
            if (terminalFailure is not null)
            {
                throw new InvalidOperationException("SFU publisher reconnect is no longer available.", terminalFailure);
            }

            var connection = GetConnection();
            if (connection is { IsConnected: true } && !ReferenceEquals(connection, previous))
            {
                return connection;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HeartbeatLoopAsync(ClientWebSocket websocket, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(HeartbeatInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await SendJsonAsync(websocket, new { type = "ping" }, cancellationToken).ConfigureAwait(false);
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
                "SFU mute state could not be restored channel={ChannelId} error_type={ErrorType} error={Error}",
                ChannelId,
                ex.GetType().Name,
                ex.Message);
        }
    }

    private Task SendJsonAsync(
        DirectSfuConnection connection,
        object message,
        CancellationToken cancellationToken)
        => SendJsonAsync(connection.WebSocket, message, cancellationToken);

    private async Task SendJsonAsync(
        ClientWebSocket websocket,
        object message,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (websocket.State != WebSocketState.Open)
            {
                throw new InvalidOperationException("SFU signaling WebSocket is not open.");
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(SendTimeout);
            await websocket.SendAsync(
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

    private Exception? GetTerminalFailure()
    {
        lock (_gate)
        {
            return _terminalFailure;
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
            _terminalFailure = null;
        }

        runCts?.Cancel();
        await AwaitQuietlyAsync(runTask, CancellationToken.None).ConfigureAwait(false);
        runCts?.Dispose();
    }

    private void FailSession(Exception exception)
    {
        lock (_gate)
        {
            _terminalFailure ??= exception;
            _connected.TrySetException(_terminalFailure);
            _trackEnded?.TrySetException(_terminalFailure);
        }

        _logger.LogError(
            "SFU publisher reconnect stopped channel={ChannelId} error_type={ErrorType} error={Error}",
            ChannelId,
            exception.GetType().Name,
            exception.Message);
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
        catch (OperationCanceledException) when (task.IsCanceled)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                "SFU publisher task stopped with an error channel={ChannelId} error_type={ErrorType} error={Error}",
                ChannelId,
                ex.GetType().Name,
                ex.Message);
        }
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class DirectSfuConnection : IDisposable
    {
        private int _closed;
        private long _connectedAtTimestamp;
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
        public bool HasBeenConnected => Volatile.Read(ref _connectedAtTimestamp) != 0;
        public TimeSpan ConnectedDuration
            => HasBeenConnected
                ? Stopwatch.GetElapsedTime(Volatile.Read(ref _connectedAtTimestamp))
                : TimeSpan.Zero;

        public void SetFailure(Exception failure) => Interlocked.CompareExchange(ref _failure, failure, null);

        public void MarkConnected()
            => Interlocked.CompareExchange(ref _connectedAtTimestamp, Stopwatch.GetTimestamp(), 0);

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
