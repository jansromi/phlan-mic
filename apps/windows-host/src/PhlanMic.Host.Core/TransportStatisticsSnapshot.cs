namespace PhlanMic.Host.Core;

public sealed record TransportStatisticsSnapshot(
    long ControlMessagesReceived,
    long ControlMessagesSent,
    long ControlTimeoutCount,
    long ProtocolErrorCount,
    long AudioPacketsRejected,
    long DuplicatePackets,
    long OutOfOrderPackets,
    long DecodeFailureCount)
{
    public static TransportStatisticsSnapshot Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0);
}
