namespace PhlanMic.Host.Core;

public sealed record StreamRobustnessConfig
{
    public int StartupPrebufferFrames { get; init; } = 4;

    public int TargetBufferedFrames { get; init; } = 3;

    public int MaxLateFrameToleranceFrames { get; init; } = 2;

    public bool ConcealMissingFramesWithSilence { get; init; } = true;

    public void Validate(StreamBufferConfig bufferConfig)
    {
        ArgumentNullException.ThrowIfNull(bufferConfig);

        if (StartupPrebufferFrames <= 0)
        {
            throw new InvalidOperationException("Startup prebuffer frames must be greater than zero.");
        }

        if (TargetBufferedFrames <= 0)
        {
            throw new InvalidOperationException("Target buffered frames must be greater than zero.");
        }

        if (MaxLateFrameToleranceFrames < 0)
        {
            throw new InvalidOperationException("Max late frame tolerance must be zero or greater.");
        }

        if (StartupPrebufferFrames > bufferConfig.MaxBufferedFrames)
        {
            throw new InvalidOperationException("Startup prebuffer frames cannot exceed the buffer capacity.");
        }

        if (TargetBufferedFrames > bufferConfig.MaxBufferedFrames)
        {
            throw new InvalidOperationException("Target buffered frames cannot exceed the buffer capacity.");
        }
    }
}
