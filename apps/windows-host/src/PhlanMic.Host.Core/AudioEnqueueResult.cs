namespace PhlanMic.Host.Core;

public enum AudioEnqueueStatus
{
    Accepted,
    AcceptedAfterDroppingOldest,
    RejectedFormatMismatch,
    RejectedBufferFull,
    RejectedDuplicateSequence,
    RejectedLateFrame
}

public readonly record struct AudioEnqueueResult(AudioEnqueueStatus Status, int BufferedFrameCount);
