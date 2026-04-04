namespace PhlanMic.Host.Core;

internal sealed class AudioInputSessionTracker
{
    private readonly object gate = new();
    private readonly string transportMode;
    private StreamSessionState state = StreamSessionState.Stopped;
    private string? localEndpoint;
    private string? remoteEndpoint;
    private long connectionId;
    private long connectionCount;
    private long disconnectCount;
    private DateTimeOffset? startedAtUtc;
    private DateTimeOffset? connectedAtUtc;
    private DateTimeOffset? lastActivityUtc;
    private DateTimeOffset? lastDisconnectedAtUtc;
    private string? statusDetail;
    private long bytesReceived;
    private long packetsReceived;
    private long framesReceived;

    public AudioInputSessionTracker(string transportMode)
    {
        if (string.IsNullOrWhiteSpace(transportMode))
        {
            throw new ArgumentException("Transport mode must be provided.", nameof(transportMode));
        }

        this.transportMode = transportMode;
    }

    public event EventHandler<StreamSessionSnapshot>? SessionChanged;

    public StreamSessionSnapshot GetSessionSnapshot()
    {
        lock (gate)
        {
            return CreateSessionSnapshot();
        }
    }

    public StreamStatisticsSnapshot GetStatisticsSnapshot(AudioStreamPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);

        lock (gate)
        {
            return new StreamStatisticsSnapshot(
                bytesReceived,
                packetsReceived,
                framesReceived,
                pipeline.AcceptedFrames,
                pipeline.RejectedFrames,
                pipeline.DroppedFrames,
                pipeline.BufferedFrameCount,
                lastActivityUtc);
        }
    }

    public void MarkListening(string? localEndpoint, DateTimeOffset atUtc, string? detail = null)
    {
        UpdateSession(
            newState: StreamSessionState.Listening,
            localEndpoint: localEndpoint,
            preserveRemoteEndpoint: true,
            atUtc: atUtc,
            detail: detail,
            incrementConnectionCount: false,
            incrementDisconnectCount: false,
            resetConnectedAtUtc: false);
    }

    public void MarkConnected(string? remoteEndpoint, DateTimeOffset atUtc, string? detail = null)
    {
        UpdateSession(
            newState: StreamSessionState.Connected,
            localEndpoint: null,
            preserveRemoteEndpoint: false,
            atUtc: atUtc,
            detail: detail,
            incrementConnectionCount: true,
            incrementDisconnectCount: false,
            resetConnectedAtUtc: false,
            remoteEndpoint: remoteEndpoint);
    }

    public void MarkStreaming(DateTimeOffset atUtc, string? detail = null)
    {
        UpdateSession(
            newState: StreamSessionState.Streaming,
            localEndpoint: null,
            preserveRemoteEndpoint: true,
            atUtc: atUtc,
            detail: detail,
            incrementConnectionCount: false,
            incrementDisconnectCount: false,
            resetConnectedAtUtc: false);
    }

    public void MarkDisconnected(DateTimeOffset atUtc, string? detail = null)
    {
        UpdateSession(
            newState: StreamSessionState.Disconnected,
            localEndpoint: null,
            preserveRemoteEndpoint: true,
            atUtc: atUtc,
            detail: detail,
            incrementConnectionCount: false,
            incrementDisconnectCount: true,
            resetConnectedAtUtc: true);
    }

    public void MarkFaulted(DateTimeOffset atUtc, string detail)
    {
        UpdateSession(
            newState: StreamSessionState.Faulted,
            localEndpoint: null,
            preserveRemoteEndpoint: true,
            atUtc: atUtc,
            detail: detail,
            incrementConnectionCount: false,
            incrementDisconnectCount: false,
            resetConnectedAtUtc: false);
    }

    public void MarkStopped(DateTimeOffset atUtc, string? detail = null)
    {
        UpdateSession(
            newState: StreamSessionState.Stopped,
            localEndpoint: null,
            preserveRemoteEndpoint: true,
            atUtc: atUtc,
            detail: detail,
            incrementConnectionCount: false,
            incrementDisconnectCount: false,
            resetConnectedAtUtc: true);
    }

    public void RecordTraffic(int bytes, int packets, int frames, DateTimeOffset atUtc)
    {
        if (bytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes), "Byte count cannot be negative.");
        }

        if (packets < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(packets), "Packet count cannot be negative.");
        }

        if (frames < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frames), "Frame count cannot be negative.");
        }

        lock (gate)
        {
            bytesReceived += bytes;
            packetsReceived += packets;
            framesReceived += frames;
            lastActivityUtc = atUtc;
        }
    }

    private void UpdateSession(
        StreamSessionState newState,
        string? localEndpoint,
        bool preserveRemoteEndpoint,
        DateTimeOffset atUtc,
        string? detail,
        bool incrementConnectionCount,
        bool incrementDisconnectCount,
        bool resetConnectedAtUtc,
        string? remoteEndpoint = null)
    {
        StreamSessionSnapshot? snapshotToPublish = null;

        lock (gate)
        {
            var previous = CreateSessionSnapshot();

            startedAtUtc ??= atUtc;

            if (localEndpoint is not null)
            {
                this.localEndpoint = localEndpoint;
            }

            if (!preserveRemoteEndpoint)
            {
                this.remoteEndpoint = remoteEndpoint;
            }

            if (incrementConnectionCount)
            {
                connectionCount++;
                connectionId = connectionCount;
                connectedAtUtc = atUtc;
                lastDisconnectedAtUtc = null;
            }

            if (incrementDisconnectCount)
            {
                disconnectCount++;
                lastDisconnectedAtUtc = atUtc;
            }

            if (resetConnectedAtUtc)
            {
                connectedAtUtc = null;
            }

            state = newState;
            statusDetail = detail;

            if (newState is StreamSessionState.Streaming)
            {
                lastActivityUtc ??= atUtc;
            }

            var current = CreateSessionSnapshot();
            if (!Equals(previous, current))
            {
                snapshotToPublish = current;
            }
        }

        if (snapshotToPublish is not null)
        {
            SessionChanged?.Invoke(this, snapshotToPublish);
        }
    }

    private StreamSessionSnapshot CreateSessionSnapshot() =>
        new(
            state,
            transportMode,
            localEndpoint,
            remoteEndpoint,
            connectionId,
            connectionCount,
            disconnectCount,
            startedAtUtc,
            connectedAtUtc,
            lastActivityUtc,
            lastDisconnectedAtUtc,
            statusDetail);
}
