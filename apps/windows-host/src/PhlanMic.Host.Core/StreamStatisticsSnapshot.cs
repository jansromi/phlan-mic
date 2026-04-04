namespace PhlanMic.Host.Core;

public sealed record StreamStatisticsSnapshot(
    long BytesReceived,
    long PacketsReceived,
    long FramesReceived,
    long AcceptedFrames,
    long RejectedFrames,
    long DroppedFrames,
    int BufferedFrameCount,
    DateTimeOffset? LastActivityUtc);
