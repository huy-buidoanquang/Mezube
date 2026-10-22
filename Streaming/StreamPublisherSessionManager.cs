using Mezube.Bot;
using Mezube.Media;
using Mezon.Net.Sdk;
using Microsoft.Extensions.Logging;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Channels;

namespace Mezube.Streaming;

public sealed class StreamClientHolder
{
    private MezonClient? _client;

    public void SetClient(MezonClient client) => _client = client;

    public MezonClient GetClient() => _client ?? throw new InvalidOperationException("Mezon client is not ready.");
}

public sealed record StreamPublishGrant(Uri WhipUrl, string Token);

/// <summary>
/// In-process WHIP publisher. It owns the WebRTC peer connection and the
/// Ogg/Opus playback loop for one stream channel.
/// </summary>
public sealed class StreamPublisherSessionManager : IAsyncDisposable
{
    private readonly BotOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<StreamPublisherSessionManager> _logger;
    private readonly ConcurrentDictionary<long, StreamPublisherSession> _sessions = new();
    private readonly object _gate = new();

    public StreamPublisherSessionManager(
        BotOptions options,
        IHttpClientFactory httpClientFactory,
        ILogger<StreamPublisherSessionManager> logger)
    {
        _options = options;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public StreamPublisherSession GetOrCreate(long channelId)
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

            if (_sessions.Count >= _options.StreamMaxSessions)
            {
                throw new StreamPublishException(
                    StreamFailureKind.Unavailable,
                    "Stream publisher session capacity is exhausted.");
            }

            var session = new StreamPublisherSession(
                _options,
                _httpClientFactory.CreateClient(nameof(MezonCdnUploader)),
                _logger,
                channelId);
            _sessions[channelId] = session;
            return session;
        }
    }

    public bool TryGet(long channelId, out StreamPublisherSession session)
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
        await Task.WhenAll(sessions.Select(session => session.DisposeAsync().AsTask())).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => new(DisposeAllAsync());
}

public sealed class StreamPublisherSession : IAsyncDisposable
{
    internal static readonly byte[] SilenceFrame = [0xF8, 0xFF, 0xFE];
    private static readonly (byte[] Packet, uint Samples) Silence = (SilenceFrame, 960);
    private static readonly TimeSpan DeleteTimeout = TimeSpan.FromSeconds(3);

    private readonly BotOptions _options;
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetimeCts = new();

    private TaskCompletionSource _connected = NewSignal();
    private Task? _runTask;
    private Task? _senderTask;
    private CancellationTokenSource? _runCts;
    private StreamConnection? _connection;
    private TrackPlayback? _track;
    private Func<CancellationToken, Task<StreamPublishGrant>>? _grantProvider;
    private Exception? _terminalFailure;
    private Exception? _lastFailure;
    private Uri? _resource;
    private bool _started;
    private bool _stopped;
    private bool _paused;

    internal StreamPublisherSession(BotOptions options, HttpClient httpClient, ILogger logger, long channelId)
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
        Func<CancellationToken, Task<StreamPublishGrant>> grantProvider,
        CancellationToken cancellationToken)
    {
        Task connectedTask;
        var startedHere = false;
        lock (_gate)
        {
            if (_stopped)
            {
                throw new ObjectDisposedException(nameof(StreamPublisherSession));
            }

            _grantProvider = grantProvider;
            if (!_started)
            {
                _started = true;
                _runCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
                var runToken = _runCts.Token;
                _runTask = Task.Run(() => RunReconnectLoopAsync(runToken), CancellationToken.None);
                _senderTask = Task.Factory.StartNew(
                    () => SendLoop(runToken),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default);
                startedHere = true;
            }

            connectedTask = _connected.Task;
        }

        try
        {
            await connectedTask.WaitAsync(
                    TimeSpan.FromMilliseconds(_options.StreamConnectTimeoutMs),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var failure = ex is TimeoutException
                ? new StreamPublishException(
                    StreamFailureKind.Unavailable,
                    "Timed out connecting to the streaming server.",
                    GetLastFailure())
                : ex;
            if (startedHere)
            {
                await StopConnectionLoopAsync().ConfigureAwait(false);
            }

            if (ReferenceEquals(failure, ex))
            {
                throw;
            }

            throw failure;
        }
    }

    internal void BeginStream(StreamMediaSource media)
    {
        var packets = Channel.CreateBounded<(byte[] Packet, uint Samples)>(new BoundedChannelOptions(4)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var cancellation = new CancellationTokenSource();
        TrackPlayback track;
        TrackPlayback? previous;
        lock (_gate)
        {
            if (_stopped)
            {
                throw new ObjectDisposedException(nameof(StreamPublisherSession));
            }

            track = new TrackPlayback(
                packets.Reader,
                cancellation,
                NewSignal(),
                Task.Run(() => ProduceAsync(media, packets.Writer, cancellation.Token)));
            previous = _track;
            _track = track;
            _paused = false;
            if (_terminalFailure is { } failure)
            {
                track.Ended.TrySetException(failure);
            }
        }

        End(previous);
    }

    public Task WaitUntilTrackEndedAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_track is null)
            {
                return Task.FromException(new InvalidOperationException("No active stream track."));
            }

            return _track.Ended.Task.WaitAsync(cancellationToken);
        }
    }

    public void SetPaused(bool paused)
    {
        lock (_gate)
        {
            if (!_stopped)
            {
                _paused = paused;
            }
        }
    }

    public void EndTrack()
    {
        TrackPlayback? track;
        lock (_gate)
        {
            track = _track;
        }

        End(track);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? runTask;
        Task? senderTask;
        CancellationTokenSource? runCts;
        TaskCompletionSource connected;
        TrackPlayback? track;
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            track = _track;
            runCts = _runCts;
            runTask = _runTask;
            senderTask = _senderTask;
            connected = _connected;
        }

        runCts?.Cancel();
        connected.TrySetCanceled();
        track?.Cancellation.Cancel();
        track?.Ended.TrySetCanceled();
        await AwaitQuietlyAsync(senderTask).ConfigureAwait(false);
        await AwaitQuietlyAsync(track?.Producer).ConfigureAwait(false);
        await AwaitQuietlyAsync(runTask).ConfigureAwait(false);
        if (GetResource() is { } resource)
        {
            await DeleteResourceAsync(resource).ConfigureAwait(false);
        }

        _lifetimeCts.Cancel();
        runCts?.Dispose();
        _lifetimeCts.Dispose();
    }

    public ValueTask DisposeAsync() => new(StopAsync(CancellationToken.None));

    internal (byte[] Packet, uint Samples) NextPacket()
    {
        TrackPlayback? track;
        bool paused;
        lock (_gate)
        {
            track = _track;
            paused = _paused;
        }

        if (track is null || paused || track.Ended.Task.IsCompleted)
        {
            return Silence;
        }

        if (track.Packets.TryRead(out var frame))
        {
            return frame;
        }

        var completion = track.Packets.Completion;
        if (completion.IsCompleted)
        {
            if (completion.Exception?.InnerException is { } error)
            {
                track.Ended.TrySetException(ToTrackFailure(error));
            }
            else
            {
                track.Ended.TrySetResult();
            }
        }

        return Silence;
    }

    internal static Exception ToTrackFailure(Exception ex)
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is InvalidDataException or HttpRequestException or IOException)
            {
                return new MediaPrepException("Audio source could not be read.", ex);
            }
        }

        return new StreamPublishException(StreamFailureKind.Unavailable, "Stream audio playback failed.", ex);
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

    private async Task RunReconnectLoopAsync(CancellationToken cancellationToken)
    {
        var attempts = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await ConnectAndRunAsync(cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (result.WasStable)
            {
                attempts = 0;
            }

            var failure = result.Failure;
            SetLastFailure(failure);
            _logger.LogWarning(
                "Stream publisher connection failed channel={ChannelId} kind={Kind} error_type={ErrorType} error={Error}",
                ChannelId,
                failure.Kind,
                (failure.InnerException ?? failure).GetType().Name,
                failure.Message);

            if (failure.Kind != StreamFailureKind.Unavailable)
            {
                FailSession(failure);
                return;
            }

            if (attempts >= _options.StreamReconnectMaxAttempts)
            {
                FailSession(new StreamPublishException(
                    StreamFailureKind.Unavailable,
                    $"Stream publisher reconnect limit reached after {_options.StreamReconnectMaxAttempts} attempts.",
                    failure));
                return;
            }

            attempts++;
            var delay = StreamReconnectPolicy.GetDelay(
                attempts,
                _options.StreamReconnectBackoffMs,
                _options.StreamReconnectMaxBackoffMs,
                _options.StreamReconnectJitterMs);
            _logger.LogWarning(
                "Stream publisher reconnect scheduled channel={ChannelId} attempt={Attempt}/{MaxAttempts} delay_ms={DelayMs}",
                ChannelId,
                attempts,
                _options.StreamReconnectMaxAttempts,
                (int)delay.TotalMilliseconds);

            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private readonly record struct ConnectionRunResult(bool WasStable, StreamPublishException Failure);

    private async Task<ConnectionRunResult> ConnectAndRunAsync(CancellationToken cancellationToken)
    {
        StreamConnection? connection = null;
        StreamPublishException failure;
        var wasStable = false;
        try
        {
            using (var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                attemptCts.CancelAfter(_options.StreamConnectTimeoutMs);
                var grant = await GetGrantAsync(attemptCts.Token).ConfigureAwait(false);
                connection = new StreamConnection(CreateAudioPublisherPeerConnection());
                AttachConnectionStateHandler(connection);
                var offer = connection.PeerConnection.createOffer();
                await connection.PeerConnection.setLocalDescription(offer).ConfigureAwait(false);
                var answer = await PostOfferAsync(grant, offer.sdp, attemptCts.Token).ConfigureAwait(false);
                var applied = connection.PeerConnection.setRemoteDescription(new RTCSessionDescriptionInit
                {
                    type = RTCSdpType.answer,
                    sdp = answer
                });
                if (applied != SetDescriptionResultEnum.OK)
                {
                    throw new StreamPublishException(
                        StreamFailureKind.Denied,
                        $"The streaming server answer was rejected: {applied}.");
                }

                lock (_gate)
                {
                    attemptCts.Token.ThrowIfCancellationRequested();
                    _connection = connection;
                }

                await connection.PeerConnection.Start().ConfigureAwait(false);
                var first = await Task.WhenAny(connection.Connected.Task, connection.Closed.Task)
                    .WaitAsync(attemptCts.Token)
                    .ConfigureAwait(false);
                if (first != connection.Connected.Task)
                {
                    throw connection.Failure ?? new StreamPublishException(
                        StreamFailureKind.Unavailable,
                        "Stream WebRTC connection closed before it connected.");
                }
            }

            lock (_gate)
            {
                _connected.TrySetResult();
            }

            _logger.LogDebug("Stream publisher connected channel={ChannelId}", ChannelId);
            await connection.Closed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            failure = connection.Failure ?? new StreamPublishException(
                StreamFailureKind.Unavailable,
                "Stream WebRTC connection closed.");
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            failure = new StreamPublishException(
                StreamFailureKind.Unavailable,
                "Timed out connecting to the streaming server.",
                ex);
        }
        catch (OperationCanceledException ex)
        {
            failure = new StreamPublishException(StreamFailureKind.Unavailable, "Stream publisher stopped.", ex);
        }
        catch (StreamPublishException ex)
        {
            failure = ex;
        }
        catch (Exception ex)
        {
            failure = new StreamPublishException(
                StreamFailureKind.Unavailable,
                "Stream publisher connection failed.",
                ex);
        }
        finally
        {
            wasStable = connection is not null
                && connection.ConnectedDuration >= TimeSpan.FromMilliseconds(_options.StreamReconnectStableResetMs);
            lock (_gate)
            {
                if (ReferenceEquals(_connection, connection))
                {
                    _connection = null;
                }
            }

            connection?.Close();
            connection?.PeerConnection.Dispose();
        }

        return new ConnectionRunResult(wasStable, failure);
    }

    private async Task<StreamPublishGrant> GetGrantAsync(CancellationToken cancellationToken)
    {
        var provider = GetGrantProvider() ?? throw new StreamPublishException(
            StreamFailureKind.Unavailable,
            "Stream publish credentials are unavailable.");
        try
        {
            return await provider(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not StreamPublishException and not OperationCanceledException)
        {
            throw new StreamPublishException(
                StreamFailureKind.Unavailable,
                "Could not get stream publish credentials.",
                ex);
        }
    }

    private async Task<string> PostOfferAsync(StreamPublishGrant grant, string offerSdp, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, grant.WhipUrl)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(offerSdp))
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/sdp");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", grant.Token);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Created)
        {
            var status = (int)response.StatusCode;
            throw new StreamPublishException(
                StreamPublishException.ForWhipStatus(status),
                $"WHIP publish was refused with HTTP {status}.");
        }

        if (response.Headers.Location is not { } location)
        {
            throw new StreamPublishException(
                StreamFailureKind.Unavailable,
                "WHIP publish returned no resource location.");
        }

        SetResource(new Uri(grant.WhipUrl, location));
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteResourceAsync(Uri resource)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var timeout = new CancellationTokenSource(DeleteTimeout);
                using var response = await _httpClient.DeleteAsync(resource, timeout.Token).ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.ServiceUnavailable)
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    "Stream publisher resource delete failed channel={ChannelId} error_type={ErrorType}",
                    ChannelId,
                    ex.GetType().Name);
                return;
            }
        }
    }

    private static void AttachConnectionStateHandler(StreamConnection connection)
    {
        connection.PeerConnection.onconnectionstatechange += state =>
        {
            switch (state)
            {
                case RTCPeerConnectionState.connected:
                    connection.MarkConnected();
                    break;
                case RTCPeerConnectionState.failed:
                    connection.Fail(new StreamPublishException(
                        StreamFailureKind.Unavailable,
                        "Stream WebRTC connection failed."));
                    break;
                case RTCPeerConnectionState.closed:
                    connection.Fail(new StreamPublishException(
                        StreamFailureKind.Unavailable,
                        "Stream WebRTC connection closed."));
                    break;
            }
        };
    }

    private void SendLoop(CancellationToken cancellationToken)
    {
        var pacer = new OpusRtpPacer();
        StreamConnection? current = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var connection = GetConnection();
                if (connection is not { IsConnected: true })
                {
                    cancellationToken.WaitHandle.WaitOne(20);
                    continue;
                }

                if (!ReferenceEquals(connection, current))
                {
                    current = connection;
                    pacer.Start();
                }

                pacer.Wait(cancellationToken);
                var (packet, duration) = NextPacket();
                var samples = duration + pacer.LagSamples();
                try
                {
                    connection.PeerConnection.SendAudio(samples, packet);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    connection.Fail(new StreamPublishException(
                        StreamFailureKind.Unavailable,
                        "Stream audio send failed.",
                        ex));
                    continue;
                }

                pacer.Account(samples);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            FailSession(new StreamPublishException(StreamFailureKind.Unavailable, "Stream audio sender stopped.", ex));
        }
    }

    private async Task ProduceAsync(
        StreamMediaSource media,
        ChannelWriter<(byte[] Packet, uint Samples)> writer,
        CancellationToken cancellationToken)
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
                await ReadPacketsAsync(file, writer, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                using var response = await _httpClient.GetAsync(
                        media.HttpUri!,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await ReadPacketsAsync(body, writer, cancellationToken).ConfigureAwait(false);
            }

            writer.TryComplete();
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            writer.TryComplete();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Stream audio track failed channel={ChannelId} error_type={ErrorType}",
                ChannelId,
                ex.GetType().Name);
            writer.TryComplete(ex is OperationCanceledException
                ? new IOException("Audio source read timed out.", ex)
                : ex);
        }
    }

    private static async Task ReadPacketsAsync(
        Stream body,
        ChannelWriter<(byte[] Packet, uint Samples)> writer,
        CancellationToken cancellationToken)
    {
        var reader = new OggOpusReader(body);
        while (await reader.ReadPacketAsync(cancellationToken).ConfigureAwait(false) is { } packet)
        {
            await writer.WriteAsync((packet, OpusPacket.GetDurationSamples(packet)), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private StreamConnection? GetConnection()
    {
        lock (_gate)
        {
            return _connection;
        }
    }

    private Func<CancellationToken, Task<StreamPublishGrant>>? GetGrantProvider()
    {
        lock (_gate)
        {
            return _grantProvider;
        }
    }

    private Exception? GetLastFailure()
    {
        lock (_gate)
        {
            return _lastFailure;
        }
    }

    private void SetLastFailure(Exception failure)
    {
        lock (_gate)
        {
            _lastFailure = failure;
        }
    }

    private Uri? GetResource()
    {
        lock (_gate)
        {
            return _resource;
        }
    }

    private void SetResource(Uri resource)
    {
        lock (_gate)
        {
            _resource = resource;
        }
    }

    private async Task StopConnectionLoopAsync()
    {
        CancellationTokenSource? runCts;
        Task? runTask;
        Task? senderTask;
        lock (_gate)
        {
            _started = false;
            runCts = _runCts;
            runTask = _runTask;
            senderTask = _senderTask;
            _runCts = null;
            _runTask = null;
            _senderTask = null;
            _connected = NewSignal();
            _terminalFailure = null;
        }

        runCts?.Cancel();
        await AwaitQuietlyAsync(senderTask).ConfigureAwait(false);
        await AwaitQuietlyAsync(runTask).ConfigureAwait(false);
        runCts?.Dispose();
    }

    private void FailSession(StreamPublishException exception)
    {
        Exception failure;
        TrackPlayback? track;
        lock (_gate)
        {
            _terminalFailure ??= exception;
            failure = _terminalFailure;
            _connected.TrySetException(failure);
            track = _track;
        }

        track?.Ended.TrySetException(failure);
        _logger.LogError(
            "Stream publisher stopped channel={ChannelId} kind={Kind} error={Error}",
            ChannelId,
            exception.Kind,
            exception.Message);
    }

    private static void End(TrackPlayback? track)
    {
        if (track is null)
        {
            return;
        }

        track.Cancellation.Cancel();
        track.Ended.TrySetResult();
    }

    private async Task AwaitQuietlyAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (task.IsCanceled)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                "Stream publisher task stopped with an error channel={ChannelId} error_type={ErrorType} error={Error}",
                ChannelId,
                ex.GetType().Name,
                ex.Message);
        }
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record TrackPlayback(
        ChannelReader<(byte[] Packet, uint Samples)> Packets,
        CancellationTokenSource Cancellation,
        TaskCompletionSource Ended,
        Task Producer);

    private sealed class StreamConnection
    {
        private int _closed;
        private long _connectedAtTimestamp;
        private StreamPublishException? _failure;

        public StreamConnection(RTCPeerConnection peerConnection)
        {
            PeerConnection = peerConnection;
        }

        public RTCPeerConnection PeerConnection { get; }
        public TaskCompletionSource Connected { get; } = NewSignal();
        public TaskCompletionSource Closed { get; } = NewSignal();
        public bool IsConnected => PeerConnection.connectionState == RTCPeerConnectionState.connected;
        public StreamPublishException? Failure => Volatile.Read(ref _failure);

        public TimeSpan ConnectedDuration
        {
            get
            {
                var connectedAt = Volatile.Read(ref _connectedAtTimestamp);
                return connectedAt == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(connectedAt);
            }
        }

        public void MarkConnected()
        {
            Interlocked.CompareExchange(ref _connectedAtTimestamp, Stopwatch.GetTimestamp(), 0);
            Connected.TrySetResult();
        }

        public void Fail(StreamPublishException failure)
        {
            Interlocked.CompareExchange(ref _failure, failure, null);
            Closed.TrySetResult();
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0)
            {
                return;
            }

            try
            {
                PeerConnection.close();
            }
            catch (Exception)
            {
            }

            Closed.TrySetResult();
        }
    }
}
