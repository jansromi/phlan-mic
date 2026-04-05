using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PhlanMic.Host.Core;

public sealed class UdpRawPcmReceiver : IAudioInputSource
{
    private static readonly TimeSpan TimeoutPollInterval = TimeSpan.FromMilliseconds(250);
    private readonly ReceiverConfig config;
    private readonly AudioFormat format;
    private readonly AudioStreamPipeline pipeline;
    private readonly AudioInputSessionTracker tracker;
    private readonly IAudioPayloadDecoder payloadDecoder;
    private readonly object sessionGate = new();
    private ActiveTransportSession? activeSession;

    public UdpRawPcmReceiver(
        ReceiverConfig config,
        AudioFormat format,
        AudioStreamPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(pipeline);

        config.Validate();
        format.Validate();

        if (!string.Equals(config.TransportMode, ReceiverConfig.UdpRawPcmTransportMode, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"UdpRawPcmReceiver requires transport mode '{ReceiverConfig.UdpRawPcmTransportMode}'.");
        }

        this.config = config;
        this.format = format;
        this.pipeline = pipeline;
        payloadDecoder = new RawPcmAudioPayloadDecoder(format);
        tracker = new AudioInputSessionTracker(config.TransportMode);
        tracker.SessionChanged += (_, snapshot) => SessionChanged?.Invoke(this, snapshot);
    }

    public event EventHandler<StreamSessionSnapshot>? SessionChanged;

    public StreamSessionSnapshot GetSessionSnapshot() => tracker.GetSessionSnapshot();

    public StreamStatisticsSnapshot GetStatisticsSnapshot() => tracker.GetStatisticsSnapshot(pipeline);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var bindAddress = ResolveBindAddress(config.BindAddress);
        var controlListener = new TcpListener(bindAddress, config.Port);
        using var audioSocket = new UdpClient(new IPEndPoint(bindAddress, config.GetResolvedAudioPort()));
        using var runtimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        controlListener.Start();
        var localEndpoint = $"control={controlListener.LocalEndpoint}; audio={audioSocket.Client.LocalEndPoint}";
        tracker.MarkListening(localEndpoint, DateTimeOffset.UtcNow, "Listening for control and UDP audio.");

        var audioTask = RunAudioLoopAsync(audioSocket, runtimeCancellation.Token);

        try
        {
            while (!runtimeCancellation.Token.IsCancellationRequested)
            {
                TcpClient? client = null;

                try
                {
                    client = await controlListener.AcceptTcpClientAsync(runtimeCancellation.Token);
                    await HandleControlClientAsync(client, localEndpoint, runtimeCancellation.Token);
                }
                catch (OperationCanceledException) when (runtimeCancellation.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception) when (!runtimeCancellation.IsCancellationRequested)
                {
                    tracker.MarkFaulted(DateTimeOffset.UtcNow, exception.Message);
                    tracker.MarkListening(localEndpoint, DateTimeOffset.UtcNow, "Awaiting the next control client.");

                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1), runtimeCancellation.Token);
                    }
                    catch (OperationCanceledException) when (runtimeCancellation.IsCancellationRequested)
                    {
                        break;
                    }
                }
                finally
                {
                    client?.Dispose();
                }
            }
        }
        finally
        {
            if (!runtimeCancellation.IsCancellationRequested)
            {
                runtimeCancellation.Cancel();
            }

            controlListener.Stop();
            ClearActiveSession();

            try
            {
                await audioTask;
            }
            catch (OperationCanceledException) when (runtimeCancellation.IsCancellationRequested)
            {
            }

            tracker.MarkStopped(DateTimeOffset.UtcNow, "UDP receiver stopped.");
        }
    }

    private async Task HandleControlClientAsync(
        TcpClient client,
        string localEndpoint,
        CancellationToken cancellationToken)
    {
        var remoteEndpoint = (IPEndPoint?)client.Client.RemoteEndPoint
            ?? throw new InvalidOperationException("Control client remote endpoint is not available.");
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        Task<string?>? pendingReadTask = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            pendingReadTask ??= reader.ReadLineAsync(cancellationToken).AsTask();
            var timeoutTask = Task.Delay(TimeoutPollInterval, cancellationToken);
            var completedTask = await Task.WhenAny(pendingReadTask, timeoutTask);

            if (completedTask == timeoutTask)
            {
                if (TryExpireSession(DateTimeOffset.UtcNow, out var timeoutSessionId))
                {
                    tracker.RecordControlTimeout(DateTimeOffset.UtcNow);
                    await TrySendControlMessageAsync(
                        stream,
                        TransportControlMessage.CreateError("Session timed out.", timeoutSessionId),
                        cancellationToken);
                    tracker.MarkDisconnected(DateTimeOffset.UtcNow, "Control/audio activity timed out.");
                    tracker.MarkListening(localEndpoint, DateTimeOffset.UtcNow, "Awaiting the next control client.");
                    break;
                }

                continue;
            }

            var line = await pendingReadTask;
            pendingReadTask = null;
            if (line is null)
            {
                HandleControlDisconnect(localEndpoint);
                break;
            }

            var receivedAtUtc = DateTimeOffset.UtcNow;
            tracker.RecordControlMessageReceived(receivedAtUtc);

            TransportControlMessage message;
            try
            {
                message = TransportControlMessageProtocol.Deserialize(line);
            }
            catch (TransportProtocolException exception)
            {
                tracker.RecordProtocolError(receivedAtUtc);
                await TrySendControlMessageAsync(stream, TransportControlMessage.CreateError(exception.Message), cancellationToken);
                tracker.MarkDisconnected(receivedAtUtc, exception.Message);
                tracker.MarkListening(localEndpoint, receivedAtUtc, "Awaiting the next control client.");
                ClearActiveSession();
                break;
            }

            bool shouldCloseClient;
            try
            {
                shouldCloseClient = await HandleControlMessageAsync(
                    message,
                    remoteEndpoint,
                    stream,
                    localEndpoint,
                    receivedAtUtc,
                    cancellationToken);
            }
            catch (TransportProtocolException exception)
            {
                tracker.RecordProtocolError(receivedAtUtc);
                await TrySendControlMessageAsync(stream, TransportControlMessage.CreateError(exception.Message), cancellationToken);
                tracker.MarkDisconnected(receivedAtUtc, exception.Message);
                tracker.MarkListening(localEndpoint, receivedAtUtc, "Awaiting the next control client.");
                ClearActiveSession();
                break;
            }

            if (shouldCloseClient)
            {
                break;
            }
        }
    }

    private async Task<bool> HandleControlMessageAsync(
        TransportControlMessage message,
        IPEndPoint remoteEndpoint,
        Stream stream,
        string localEndpoint,
        DateTimeOffset receivedAtUtc,
        CancellationToken cancellationToken)
    {
        switch (message.GetMessageType())
        {
            case TransportControlMessageType.Hello:
            {
                if (!TryOpenSession(message, remoteEndpoint, receivedAtUtc, out var openedSession, out var error))
                {
                    tracker.RecordProtocolError(receivedAtUtc);
                    await TrySendControlMessageAsync(stream, TransportControlMessage.CreateError(error), cancellationToken);
                    tracker.MarkDisconnected(receivedAtUtc, error);
                    tracker.MarkListening(localEndpoint, receivedAtUtc, "Awaiting the next control client.");
                    ClearActiveSession();
                    return true;
                }

                tracker.MarkConnected(
                    openedSession.RemoteControlEndpoint,
                    receivedAtUtc,
                    "Control handshake accepted. Awaiting startStream.");

                await TrySendControlMessageAsync(
                    stream,
                    TransportControlMessage.CreateHelloAccepted(
                        openedSession.SessionId,
                        config.GetResolvedAudioPort(),
                        openedSession.PayloadCodec,
                        format,
                        config.KeepAliveIntervalMs,
                        config.SessionTimeoutMs,
                        "Control handshake accepted."),
                    cancellationToken);

                return false;
            }
            case TransportControlMessageType.StartStream:
            {
                if (!TryStartStream(message, receivedAtUtc, out var sessionId, out var error))
                {
                    tracker.RecordProtocolError(receivedAtUtc);
                    await TrySendControlMessageAsync(stream, TransportControlMessage.CreateError(error, sessionId), cancellationToken);
                    tracker.MarkDisconnected(receivedAtUtc, error);
                    tracker.MarkListening(localEndpoint, receivedAtUtc, "Awaiting the next control client.");
                    ClearActiveSession();
                    return true;
                }

                await TrySendControlMessageAsync(
                    stream,
                    TransportControlMessage.CreateStartAccepted(sessionId, "UDP audio stream accepted."),
                    cancellationToken);

                return false;
            }
            case TransportControlMessageType.KeepAlive:
            {
                if (!TryTouchSession(message, receivedAtUtc, out var error))
                {
                    tracker.RecordProtocolError(receivedAtUtc);
                    await TrySendControlMessageAsync(stream, TransportControlMessage.CreateError(error), cancellationToken);
                    tracker.MarkDisconnected(receivedAtUtc, error);
                    tracker.MarkListening(localEndpoint, receivedAtUtc, "Awaiting the next control client.");
                    ClearActiveSession();
                    return true;
                }

                return false;
            }
            case TransportControlMessageType.StopStream:
            {
                if (!TryStopSession(message, receivedAtUtc, out var error))
                {
                    tracker.RecordProtocolError(receivedAtUtc);
                    await TrySendControlMessageAsync(stream, TransportControlMessage.CreateError(error), cancellationToken);
                    tracker.MarkDisconnected(receivedAtUtc, error);
                    tracker.MarkListening(localEndpoint, receivedAtUtc, "Awaiting the next control client.");
                    ClearActiveSession();
                    return true;
                }

                tracker.MarkDisconnected(receivedAtUtc, "Client requested stream stop.");
                tracker.MarkListening(localEndpoint, receivedAtUtc, "Awaiting the next control client.");
                return true;
            }
            default:
            {
                const string error = "Server only accepts hello, startStream, keepAlive, and stopStream from the client.";
                tracker.RecordProtocolError(receivedAtUtc);
                await TrySendControlMessageAsync(stream, TransportControlMessage.CreateError(error), cancellationToken);
                tracker.MarkDisconnected(receivedAtUtc, error);
                tracker.MarkListening(localEndpoint, receivedAtUtc, "Awaiting the next control client.");
                ClearActiveSession();
                return true;
            }
        }
    }

    private async Task RunAudioLoopAsync(UdpClient audioSocket, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult datagram;

            try
            {
                datagram = await audioSocket.ReceiveAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                tracker.MarkFaulted(DateTimeOffset.UtcNow, exception.Message);

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                continue;
            }

            HandleAudioPacket(datagram.Buffer, datagram.RemoteEndPoint);
        }
    }

    private void HandleAudioPacket(byte[] datagram, IPEndPoint remoteEndpoint)
    {
        var receivedAtUtc = DateTimeOffset.UtcNow;

        TransportAudioPacket packet;
        try
        {
            packet = TransportAudioPacketSerializer.Deserialize(datagram);
        }
        catch (TransportProtocolException)
        {
            tracker.RecordProtocolError(receivedAtUtc);
            tracker.RecordRejectedPacket(receivedAtUtc);
            return;
        }

        if (!TryValidateAudioPacket(packet, remoteEndpoint, receivedAtUtc, out var session, out var assessment))
        {
            tracker.RecordRejectedPacket(receivedAtUtc);
            return;
        }

        if (assessment.IsDuplicate)
        {
            tracker.RecordDuplicatePacket(receivedAtUtc);
            tracker.RecordRejectedPacket(receivedAtUtc);
            return;
        }

        if (assessment.IsOutOfOrder)
        {
            tracker.RecordOutOfOrderPacket(receivedAtUtc);
        }

        AudioFrame frame;
        try
        {
            frame = payloadDecoder.Decode(
                packet.SequenceNumber,
                packet.CapturedAtUtc,
                packet.PayloadCodec,
                packet.Payload);
        }
        catch (TransportProtocolException)
        {
            tracker.RecordDecodeFailure(receivedAtUtc);
            tracker.RecordRejectedPacket(receivedAtUtc);
            return;
        }

        tracker.RecordTraffic(datagram.Length, packets: 1, frames: 1, atUtc: receivedAtUtc);

        if (TryMarkStreaming(session.SessionId, remoteEndpoint, receivedAtUtc))
        {
            tracker.MarkStreaming(receivedAtUtc, $"Receiving UDP audio packets from {remoteEndpoint}.");
        }

        pipeline.Write(frame);
    }

    private bool TryOpenSession(
        TransportControlMessage hello,
        IPEndPoint remoteEndpoint,
        DateTimeOffset atUtc,
        out ActiveTransportSession openedSession,
        out string error)
    {
        lock (sessionGate)
        {
            openedSession = null!;
            error = string.Empty;

            if (activeSession is not null)
            {
                error = "Another transport session is already active.";
                return false;
            }

            if (hello.SupportedCodecs is null ||
                !hello.SupportedCodecs.Any(codec => string.Equals(codec, config.PayloadCodec, StringComparison.OrdinalIgnoreCase)))
            {
                error = $"Client does not support required payload codec '{config.PayloadCodec}'.";
                return false;
            }

            activeSession = new ActiveTransportSession(
                Guid.NewGuid(),
                remoteEndpoint.Address,
                remoteEndpoint.ToString(),
                AudioPayloadCodec.RawPcm16,
                atUtc,
                hello.SessionName);

            openedSession = activeSession;
            return true;
        }
    }

    private bool TryStartStream(
        TransportControlMessage startMessage,
        DateTimeOffset atUtc,
        out Guid sessionId,
        out string error)
    {
        lock (sessionGate)
        {
            sessionId = Guid.Empty;
            error = string.Empty;

            if (activeSession is null)
            {
                error = "No active control session exists.";
                return false;
            }

            sessionId = activeSession.SessionId;

            if (startMessage.GetRequiredSessionId() != activeSession.SessionId)
            {
                error = "StartStream session id does not match the active control session.";
                return false;
            }

            if (startMessage.GetRequiredPayloadCodec() is not AudioPayloadCodec.RawPcm16)
            {
                error = "Only RawPcm16 payloads are supported in UdpRawPcm mode.";
                return false;
            }

            var requestedFormat = new AudioFormat
            {
                SampleRate = startMessage.SampleRate ?? 0,
                Channels = startMessage.Channels ?? 0,
                BitsPerSample = startMessage.BitsPerSample ?? 0,
                FrameDurationMs = startMessage.FrameDurationMs ?? 0
            };

            if (!Equals(requestedFormat, format))
            {
                error =
                    $"StartStream audio format {requestedFormat.SampleRate}Hz/{requestedFormat.Channels}ch/{requestedFormat.BitsPerSample}bit/{requestedFormat.FrameDurationMs}ms " +
                    $"does not match host format {format.SampleRate}Hz/{format.Channels}ch/{format.BitsPerSample}bit/{format.FrameDurationMs}ms.";
                return false;
            }

            pipeline.ResetForNewStream();
            activeSession.StreamStarted = true;
            activeSession.Streaming = false;
            activeSession.AudioEndpoint = null;
            activeSession.LastActivityUtc = atUtc;
            activeSession.SequenceTracker.Reset();
            return true;
        }
    }

    private bool TryTouchSession(TransportControlMessage keepAlive, DateTimeOffset atUtc, out string error)
    {
        lock (sessionGate)
        {
            error = string.Empty;

            if (activeSession is null)
            {
                error = "No active control session exists.";
                return false;
            }

            if (keepAlive.GetRequiredSessionId() != activeSession.SessionId)
            {
                error = "KeepAlive session id does not match the active control session.";
                return false;
            }

            activeSession.LastActivityUtc = atUtc;
            return true;
        }
    }

    private bool TryStopSession(TransportControlMessage stopMessage, DateTimeOffset atUtc, out string error)
    {
        lock (sessionGate)
        {
            error = string.Empty;

            if (activeSession is null)
            {
                error = "No active control session exists.";
                return false;
            }

            if (stopMessage.GetRequiredSessionId() != activeSession.SessionId)
            {
                error = "StopStream session id does not match the active control session.";
                return false;
            }

            activeSession.LastActivityUtc = atUtc;
            ResetActiveStreamStateLocked();
            return true;
        }
    }

    private bool TryValidateAudioPacket(
        TransportAudioPacket packet,
        IPEndPoint remoteEndpoint,
        DateTimeOffset atUtc,
        out ActiveTransportSession session,
        out PacketSequenceAssessment assessment)
    {
        lock (sessionGate)
        {
            session = null!;
            assessment = default;

            if (activeSession is null || !activeSession.StreamStarted)
            {
                return false;
            }

            if (packet.SessionId != activeSession.SessionId)
            {
                return false;
            }

            if (!Equals(remoteEndpoint.Address, activeSession.RemoteAddress))
            {
                return false;
            }

            if (packet.PayloadCodec != activeSession.PayloadCodec)
            {
                return false;
            }

            activeSession.LastActivityUtc = atUtc;
            activeSession.AudioEndpoint ??= remoteEndpoint.ToString();
            assessment = activeSession.SequenceTracker.Record(packet.SequenceNumber);
            session = activeSession;
            return true;
        }
    }

    private bool TryMarkStreaming(Guid sessionId, IPEndPoint remoteEndpoint, DateTimeOffset atUtc)
    {
        lock (sessionGate)
        {
            if (activeSession is null ||
                activeSession.SessionId != sessionId ||
                activeSession.Streaming)
            {
                return false;
            }

            activeSession.Streaming = true;
            activeSession.AudioEndpoint = remoteEndpoint.ToString();
            activeSession.LastActivityUtc = atUtc;
            return true;
        }
    }

    private bool TryExpireSession(DateTimeOffset atUtc, out Guid sessionId)
    {
        lock (sessionGate)
        {
            sessionId = Guid.Empty;

            if (activeSession is null)
            {
                return false;
            }

            if (atUtc - activeSession.LastActivityUtc <= TimeSpan.FromMilliseconds(config.SessionTimeoutMs))
            {
                return false;
            }

            sessionId = activeSession.SessionId;
            ResetActiveStreamStateLocked();
            return true;
        }
    }

    private void HandleControlDisconnect(string localEndpoint)
    {
        if (ClearActiveSession())
        {
            var atUtc = DateTimeOffset.UtcNow;
            tracker.MarkDisconnected(atUtc, "Control client disconnected.");
            tracker.MarkListening(localEndpoint, atUtc, "Awaiting the next control client.");
        }
    }

    private bool ClearActiveSession()
    {
        lock (sessionGate)
        {
            if (activeSession is null)
            {
                return false;
            }

            ResetActiveStreamStateLocked();
            return true;
        }
    }

    private void ResetActiveStreamStateLocked()
    {
        pipeline.ResetForNewStream();
        activeSession = null;
    }

    private async Task TrySendControlMessageAsync(
        Stream stream,
        TransportControlMessage message,
        CancellationToken cancellationToken)
    {
        try
        {
            await TransportControlMessageProtocol.WriteAsync(stream, message, cancellationToken);
            tracker.RecordControlMessageSent(DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static IPAddress ResolveBindAddress(string bindAddress)
    {
        if (IPAddress.TryParse(bindAddress, out var parsed))
        {
            return parsed;
        }

        if (string.Equals(bindAddress, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.Loopback;
        }

        var addresses = Dns.GetHostAddresses(bindAddress);
        var address = addresses.FirstOrDefault(candidate => candidate.AddressFamily is AddressFamily.InterNetwork)
            ?? addresses.FirstOrDefault()
            ?? throw new InvalidOperationException($"Unable to resolve bind address '{bindAddress}'.");

        return address;
    }

    private sealed class ActiveTransportSession
    {
        public ActiveTransportSession(
            Guid sessionId,
            IPAddress remoteAddress,
            string remoteControlEndpoint,
            AudioPayloadCodec payloadCodec,
            DateTimeOffset lastActivityUtc,
            string? sessionName)
        {
            SessionId = sessionId;
            RemoteAddress = remoteAddress;
            RemoteControlEndpoint = remoteControlEndpoint;
            PayloadCodec = payloadCodec;
            LastActivityUtc = lastActivityUtc;
            SessionName = sessionName;
        }

        public Guid SessionId { get; }

        public IPAddress RemoteAddress { get; }

        public string RemoteControlEndpoint { get; }

        public AudioPayloadCodec PayloadCodec { get; }

        public string? SessionName { get; }

        public DateTimeOffset LastActivityUtc { get; set; }

        public bool StreamStarted { get; set; }

        public bool Streaming { get; set; }

        public string? AudioEndpoint { get; set; }

        public PacketSequenceTracker SequenceTracker { get; } = new();
    }
}
