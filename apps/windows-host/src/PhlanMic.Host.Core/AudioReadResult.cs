namespace PhlanMic.Host.Core;

public enum AudioReadStatus
{
    FrameAvailable,
    WaitingForFrame
}

public readonly record struct AudioReadResult(AudioReadStatus Status, AudioFrame? Frame, int BufferedFrameCount);
