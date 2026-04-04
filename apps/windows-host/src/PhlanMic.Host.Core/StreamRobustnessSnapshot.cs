namespace PhlanMic.Host.Core;

public sealed record StreamRobustnessSnapshot(
    StreamRobustnessState State,
    long? ExpectedNextSequence,
    long? HighestReceivedSequence,
    long SequenceGapsObserved,
    long LateFramesArrived,
    long LateFramesDropped,
    long MissingFramesDetected,
    long SilenceFramesInserted,
    int CurrentPrebufferDepth,
    int LargestObservedGap,
    int StartupPrebufferFrames,
    int TargetBufferedFrames,
    int MaxLateFrameToleranceFrames,
    int EstimatedBufferLatencyMs);
