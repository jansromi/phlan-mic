namespace PhlanMic.Host.Core;

public sealed record StreamBufferConfig
{
    public int MaxBufferedFrames { get; init; } = 8;

    public bool DropOldestWhenFull { get; init; } = true;

    public void Validate()
    {
        if (MaxBufferedFrames <= 0)
        {
            throw new InvalidOperationException("Max buffered frames must be greater than zero.");
        }
    }
}

