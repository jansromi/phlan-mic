namespace PhlanMic.WindowsHost;

internal sealed record DebugPipelineDrainSnapshot(
    long DrainedFrames,
    long DrainedBytes,
    long? LastSequenceNumber,
    DateTimeOffset? LastFrameCapturedAtUtc,
    DateTimeOffset? LastDrainedAtUtc);
