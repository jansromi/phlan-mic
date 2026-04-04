namespace PhlanMic.Host.Core;

public enum AudioEnqueueStatus
{
    Accepted,
    AcceptedAfterDroppingOldest,
    RejectedFormatMismatch,
    RejectedBufferFull
}

public readonly record struct AudioEnqueueResult(AudioEnqueueStatus Status, int BufferedFrameCount);
